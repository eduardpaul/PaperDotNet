from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .view_layout import ViewLayout

@dataclass
class ViewResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The columns property
    columns: Optional[list[str]] = None
    # The filter property
    filter: Optional[str] = None
    # The groupBy property
    group_by: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The isDefault property
    is_default: Optional[bool] = None
    # The layout property
    layout: Optional[ViewLayout] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The name property
    name: Optional[str] = None
    # The orderBy property
    order_by: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ViewResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ViewResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ViewResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .view_layout import ViewLayout

        from .view_layout import ViewLayout

        fields: dict[str, Callable[[Any], None]] = {
            "columns": lambda n : setattr(self, 'columns', n.get_collection_of_primitive_values(str)),
            "filter": lambda n : setattr(self, 'filter', n.get_str_value()),
            "groupBy": lambda n : setattr(self, 'group_by', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "isDefault": lambda n : setattr(self, 'is_default', n.get_bool_value()),
            "layout": lambda n : setattr(self, 'layout', n.get_enum_value(ViewLayout)),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "orderBy": lambda n : setattr(self, 'order_by', n.get_str_value()),
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
        writer.write_collection_of_primitive_values("columns", self.columns)
        writer.write_str_value("filter", self.filter)
        writer.write_str_value("groupBy", self.group_by)
        writer.write_uuid_value("id", self.id)
        writer.write_bool_value("isDefault", self.is_default)
        writer.write_enum_value("layout", self.layout)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_str_value("name", self.name)
        writer.write_str_value("orderBy", self.order_by)
        writer.write_additional_data_value(self.additional_data)
    

