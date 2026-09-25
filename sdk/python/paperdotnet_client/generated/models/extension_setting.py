from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .extension_setting_type import ExtensionSettingType

@dataclass
class ExtensionSetting(AdditionalDataHolder, Parsable):
    """
    A tenant-level setting of an extension.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The choices property
    choices: Optional[list[str]] = None
    # The description property
    description: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The required property
    required: Optional[bool] = None
    # The type property
    type: Optional[ExtensionSettingType] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ExtensionSetting:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ExtensionSetting
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ExtensionSetting()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .extension_setting_type import ExtensionSettingType

        from .extension_setting_type import ExtensionSettingType

        fields: dict[str, Callable[[Any], None]] = {
            "choices": lambda n : setattr(self, 'choices', n.get_collection_of_primitive_values(str)),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "required": lambda n : setattr(self, 'required', n.get_bool_value()),
            "type": lambda n : setattr(self, 'type', n.get_enum_value(ExtensionSettingType)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_primitive_values("choices", self.choices)
        writer.write_str_value("description", self.description)
        writer.write_str_value("name", self.name)
        writer.write_bool_value("required", self.required)
        writer.write_enum_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)
    

