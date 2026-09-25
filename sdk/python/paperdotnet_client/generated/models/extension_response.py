from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .extension_contributions import ExtensionContributions
    from .extension_scope import ExtensionScope
    from .extension_setting import ExtensionSetting

@dataclass
class ExtensionResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # What an extension contributes (for the catalog API and validation).
    contributions: Optional[ExtensionContributions] = None
    # The description property
    description: Optional[str] = None
    # The enabled property
    enabled: Optional[bool] = None
    # The id property
    id: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The publisher property
    publisher: Optional[str] = None
    # The scopes property
    scopes: Optional[list[ExtensionScope]] = None
    # The settings property
    settings: Optional[list[ExtensionSetting]] = None
    # The version property
    version: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ExtensionResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ExtensionResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ExtensionResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .extension_contributions import ExtensionContributions
        from .extension_scope import ExtensionScope
        from .extension_setting import ExtensionSetting

        from .extension_contributions import ExtensionContributions
        from .extension_scope import ExtensionScope
        from .extension_setting import ExtensionSetting

        fields: dict[str, Callable[[Any], None]] = {
            "contributions": lambda n : setattr(self, 'contributions', n.get_object_value(ExtensionContributions)),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "id": lambda n : setattr(self, 'id', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "publisher": lambda n : setattr(self, 'publisher', n.get_str_value()),
            "scopes": lambda n : setattr(self, 'scopes', n.get_collection_of_object_values(ExtensionScope)),
            "settings": lambda n : setattr(self, 'settings', n.get_collection_of_object_values(ExtensionSetting)),
            "version": lambda n : setattr(self, 'version', n.get_str_value()),
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
        writer.write_object_value("contributions", self.contributions)
        writer.write_str_value("description", self.description)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_str_value("id", self.id)
        writer.write_str_value("name", self.name)
        writer.write_str_value("publisher", self.publisher)
        writer.write_collection_of_object_values("scopes", self.scopes)
        writer.write_collection_of_object_values("settings", self.settings)
        writer.write_str_value("version", self.version)
        writer.write_additional_data_value(self.additional_data)
    

