from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .smart_folder_group import SmartFolderGroup

@dataclass
class SmartFolderGroupsResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The by property
    by: Optional[str] = None
    # The field property
    field: Optional[str] = None
    # The value property
    value: Optional[list[SmartFolderGroup]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SmartFolderGroupsResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SmartFolderGroupsResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SmartFolderGroupsResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .smart_folder_group import SmartFolderGroup

        from .smart_folder_group import SmartFolderGroup

        fields: dict[str, Callable[[Any], None]] = {
            "by": lambda n : setattr(self, 'by', n.get_str_value()),
            "field": lambda n : setattr(self, 'field', n.get_str_value()),
            "value": lambda n : setattr(self, 'value', n.get_collection_of_object_values(SmartFolderGroup)),
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
        writer.write_str_value("by", self.by)
        writer.write_str_value("field", self.field)
        writer.write_collection_of_object_values("value", self.value)
        writer.write_additional_data_value(self.additional_data)
    

